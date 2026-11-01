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
    public sealed class OwnerRigidbodyTests
    {
        static readonly List<AsyncOperation> unloading=new();
        [UnityTearDown] public IEnumerator FinishUnloads(){foreach(var op in unloading)yield return op;unloading.Clear();}
        static void Set(object obj,string name,object value)=>obj.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).SetValue(obj,value);
        [UnityTest] public IEnumerator UdpOwnerSimulatesAcrossWorkerHandoff()=>Run(false);
        [UnityTest] public IEnumerator HostAndWebSocketOwnersSimulateWithoutServerCompetition()=>Run(true);
        static IEnumerator Run(bool host)
        {
            const string root="Assets/Samples/AtlasNet Unity/0.1.0/00 Shared/Prefabs/";
            var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(root+"ClientPhysicsPlayer.prefab").GetComponent<NetworkObject>();
            var list=AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(root+"ClientPhysicsPlayerNetworkPrefabs.asset");
            NetworkManager Create(string name,ClientTransport kind=ClientTransport.Udp)
            {
                var m=new GameObject(name).AddComponent<NetworkManager>();Set(m,"playerPrefab",prefab);Set(m,"networkPrefabsLists",new[]{list});
                Set(m,"automaticLocalHandoffs",true);Set(m,"localBoundaryMargin",0.05f);
                m.ConfigureConnection("127.0.0.1",17883,kind);return m;
            }
            var server=Create("Owner physics server");var worker=Create("Owner physics worker");var udp=Create("Owner physics UDP");var web=Create("Owner physics WebSocket",ClientTransport.WebSocket);
            var managers=new[]{server,worker,udp,web};
            var scenes=managers.Select(m=>SceneManager.CreateScene(m.name,new CreateSceneParameters(LocalPhysicsMode.Physics3D))).ToArray();
            foreach(var scene in scenes)
            {
                var floor=GameObject.CreatePrimitive(PrimitiveType.Cube);floor.transform.position=new Vector3(0,-.5f,0);floor.transform.localScale=new Vector3(100,1,100);SceneManager.MoveGameObjectToScene(floor,scene);
            }
            void Prepare()
            {
                for(int i=0;i<managers.Length;i++)foreach(var o in managers[i].SpawnedObjects.ToArray())
                {
                    if(o.gameObject.scene!=scenes[i])SceneManager.MoveGameObjectToScene(o.gameObject,scenes[i]);
                    // Input is injected into the actual sample movement's FixedUpdate.
                    // Multiple editor-test clients must not read the same keyboard or render cameras.
                    foreach(var script in o.GetComponentsInChildren<MonoBehaviour>(true))if(script&&script.GetType().Namespace!="AtlasNet")script.enabled=false;
                    foreach(var camera in o.GetComponentsInChildren<Camera>(true))camera.enabled=false;
                    foreach(var audio in o.GetComponentsInChildren<AudioListener>(true))audio.enabled=false;
                }
            }
            IEnumerator Step(Action beforePhysics=null)
            {
                Prepare();yield return new WaitForFixedUpdate();Prepare();beforePhysics?.Invoke();
                foreach(var scene in scenes)scene.GetPhysicsScene().Simulate(Time.fixedDeltaTime);
                yield return null;
            }
            IEnumerator Until(Func<bool> ready,string phase)
            {
                float end=Time.realtimeSinceStartup+12;
                while(!ready()&&Time.realtimeSinceStartup<end)yield return Step();
                Assert.IsTrue(ready(),phase);
            }
            try
            {
                server.PlayerSpawnPosition=id=>new Vector3(-3,1,((long)id.Value-2)*6);
                if(host)server.StartHost();else server.StartServer();worker.StartWorker();udp.StartClient();web.StartClient();
                int count=host?3:2;
                yield return Until(()=>server.WorkerCount==2&&udp.SpawnedCount==count&&web.SpawnedCount==count&&udp.IsConnectedClient&&web.IsConnectedClient,"owner replicas ready");
                Prepare();
                foreach(var m in managers)foreach(var obj in m.SpawnedObjects)
                    Assert.AreEqual(!obj.IsOwner,obj.GetComponent<Rigidbody>().isKinematic,"Only the owning client/host may simulate this body.");
                var owner=host?web:udp;var local=owner.SpawnedObjects.Single(o=>o.IsOwner);var body=local.GetComponent<Rigidbody>();
                var movement=(Behaviour)local.GetComponent("PhysicsPlayerMovement");
                var fixedStep=movement.GetType().GetMethod("FixedUpdate",BindingFlags.Instance|BindingFlags.NonPublic);
                void Drive(){Set(movement,"input",Vector2.right);Set(movement,"yaw",0f);fixedStep.Invoke(movement,null);}
                uint epoch=local.AuthorityEpoch;
                // Isolate handoff velocity from floor friction during the motion check.
                body.useGravity=false;body.position=new Vector3(body.position.x,3,body.position.z);
                yield return Step(Drive);
                for(int i=0;i<48;i++)
                {
                    Assert.IsFalse(body.isKinematic);
                    Assert.That(body.linearVelocity.x,Is.EqualTo(5f).Within(.1f),"A worker handoff must not zero the still-controlling client's velocity.");
                    yield return Step(Drive);
                }
                yield return Until(()=>local.AuthorityEpoch>epoch,"logical authority handoff while client continues simulation");
                Assert.Greater(local.transform.position.x,.5f);Assert.IsFalse(body.isKinematic);
                var observer=host?udp:web;
                // Remote views intentionally trail moving owners by the interpolation buffer.
                // Stop before checking eventual convergence rather than demanding zero display lag.
                Set(movement,"input",Vector2.zero); body.linearVelocity=Vector3.zero;
                yield return Until(()=>observer.TryGet(local.EntityId,out var remote)&&Vector3.Distance(remote.transform.position,local.transform.position)<.3f,"remote pose catches up");
                // The real sample movement queues a physical jump only on its writer.
                body.position=new Vector3(body.position.x,1,body.position.z);body.useGravity=true;
                body.linearVelocity=new Vector3(0,0,0);Set(movement,"input",Vector2.zero);Set(movement,"jumpQueued",true);
                yield return Step(()=>fixedStep.Invoke(movement,null));
                Assert.Greater(body.linearVelocity.y,3f,"Owner Rigidbody jump must run immediately in its own physics scene.");
                foreach(var m in new[]{server,worker})foreach(var obj in m.SpawnedObjects.Where(o=>o.EntityId==local.EntityId))Assert.IsTrue(obj.GetComponent<Rigidbody>().isKinematic);
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                web.Stop();udp.Stop();worker.Stop();server.Stop();
                foreach(var m in managers)Object.DestroyImmediate(m.gameObject);
                foreach(var scene in scenes)unloading.Add(SceneManager.UnloadSceneAsync(scene));
            }
        }
    }
}
#endif
