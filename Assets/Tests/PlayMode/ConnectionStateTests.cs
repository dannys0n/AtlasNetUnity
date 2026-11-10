using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AtlasNet;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace AtlasNetDemo.Tests
{
    public sealed class PersistentDoorProbe : NetworkBehaviour
    {
        public readonly NetworkVariable<float> OpenFraction = new(0f);
        public float InitialFraction;
        public override void OnNetworkSpawn()=>InitialFraction=OpenFraction.Value;
    }
    public sealed class ConnectionStateTests
    {
        static void Set(object o,string n,object v)=>o.GetType().GetField(n,BindingFlags.NonPublic|BindingFlags.Instance).SetValue(o,v);
        static NetworkManager Create(string name,int port,ClientTransport kind=ClientTransport.Udp)
        {var m=new GameObject(name).AddComponent<NetworkManager>();m.ConfigureConnection("127.0.0.1",port,kind);return m;}
        static IEnumerator Until(Func<bool> condition,string phase)
        {float end=Time.realtimeSinceStartup+10;while(!condition()&&Time.realtimeSinceStartup<end)yield return null;Assert.IsTrue(condition(),phase);}
        [UnityTest] public IEnumerator DisconnectReasonsArriveBeforeCallbacksForUdpAndWebSocket()
        {
            var server=Create("Reason server",17881);var udp=Create("Reason UDP",17881);var web=Create("Reason WebSocket",17881,ClientTransport.WebSocket);
            int failures=0;udp.OnTransportFailure+=()=>failures++;web.OnTransportFailure+=()=>failures++;
            string udpReason=null,webReason=null;
            udp.OnClientDisconnectCallback+=_=>udpReason=udp.DisconnectReason;web.OnClientDisconnectCallback+=_=>webReason=web.DisconnectReason;
            try
            {
                server.StartServer();udp.StartClient();web.StartClient();
                yield return Until(()=>server.RemoteClientCount==2&&udp.IsConnectedClient&&web.IsConnectedClient,"both clients admitted");
                server.DisconnectClient(udp.LocalSession,"Demo ended");server.DisconnectClient(web.LocalSession,"Session is full");
                yield return Until(()=>!udp.IsRunning&&!web.IsRunning&&server.RemoteClientCount==0,"reasoned disconnects");
                Assert.AreEqual("Demo ended",udpReason);Assert.AreEqual("Session is full",webReason);Assert.AreEqual(0,failures);
                udp.StartClient();Assert.IsEmpty(udp.DisconnectReason);
                yield return Until(()=>udp.IsConnectedClient,"reconnection");
            }
            finally{web.Stop();udp.Stop();server.Stop();UnityEngine.Object.DestroyImmediate(web.gameObject);UnityEngine.Object.DestroyImmediate(udp.gameObject);UnityEngine.Object.DestroyImmediate(server.gameObject);}
        }
        [UnityTest] public IEnumerator PersistentDoorStateRestoresOnLateJoinAndInterestReentry()
        {
            var template=new GameObject("Persistent door template");template.SetActive(false);var prefab=template.AddComponent<NetworkObject>();template.AddComponent<PersistentDoorProbe>();Set(prefab,"prefabId","persistent-door-test");
            var list=ScriptableObject.CreateInstance<NetworkPrefabsList>();Set(list,"prefabs",new List<NetworkObject>{prefab});
            var server=Create("Door server",17882);var client=Create("Late door observer",17882,ClientTransport.WebSocket);Set(server,"networkPrefabsLists",new[]{list});Set(client,"networkPrefabsLists",new[]{list});
            try
            {
                server.StartServer();var door=server.Spawn(prefab,Vector3.zero,Quaternion.identity);door.GetComponent<PersistentDoorProbe>().OpenFraction.Value=.75f;
                client.StartClient();yield return Until(()=>client.SpawnedCount==1&&client.IsConnectedClient,"late join snapshot");
                Assert.AreEqual(.75f,client.SpawnedObjects.Single().GetComponent<PersistentDoorProbe>().InitialFraction);
                server.HideFrom(door,client.LocalSession);yield return Until(()=>client.SpawnedCount==0,"interest exit");
                door.GetComponent<PersistentDoorProbe>().OpenFraction.Value=.25f;server.ShowTo(door,client.LocalSession);
                yield return Until(()=>client.SpawnedCount==1,"interest reentry snapshot");
                Assert.AreEqual(.25f,client.SpawnedObjects.Single().GetComponent<PersistentDoorProbe>().InitialFraction);
            }
            finally{client.Stop();server.Stop();UnityEngine.Object.DestroyImmediate(client.gameObject);UnityEngine.Object.DestroyImmediate(server.gameObject);UnityEngine.Object.DestroyImmediate(template);UnityEngine.Object.DestroyImmediate(list);}
        }
        [UnityTest]
        public IEnumerator LocalTransformAccumulatesRotationAcrossPositionUpdates()
        {
            var template = new GameObject("Local transform template");
            template.SetActive(false);
            var prefab = template.AddComponent<NetworkObject>();
            Set(prefab, "prefabId", "local-transform-test");
            var child = new GameObject("Replicated child").transform;
            child.SetParent(template.transform, false);
            var networkTransform = template.AddComponent<NetworkTransform>();
            Set(networkTransform, "target", child);
            Set(networkTransform, "inLocalSpace", true);
            Set(networkTransform, "rotationWriter", TransformWriter.Server);
            Set(networkTransform, "rotationThreshold", 2f);
            Set(networkTransform, "interpolate", false);
            var list = ScriptableObject.CreateInstance<NetworkPrefabsList>();
            Set(list, "prefabs", new List<NetworkObject> { prefab });
            var server = Create("Local transform server", 17886);
            var client = Create("Local transform observer", 17886, ClientTransport.WebSocket);
            Set(server, "networkPrefabsLists", new[] { list });
            Set(client, "networkPrefabsLists", new[] { list });

            try
            {
                server.StartServer();
                var source = server.Spawn(prefab, Vector3.zero, Quaternion.identity).transform.GetChild(0);
                source.localPosition = Vector3.forward;
                client.StartClient();
                yield return Until(() => client.IsConnectedClient && client.SpawnedCount == 1, "late join");
                var replica = client.SpawnedObjects.Single().transform.GetChild(0);
                Assert.AreEqual(Vector3.forward, replica.localPosition);
                replica.parent.SetPositionAndRotation(new Vector3(5, 0, 0), Quaternion.Euler(0, 90, 0));

                for (int step = 1; step <= 3; step++)
                {
                    var position = new Vector3(step, 0, 1);
                    source.localPosition = position;
                    source.localRotation = Quaternion.Euler(0, step, 0);
                    yield return Until(() => replica.localPosition == position, $"local position at step {step}");
                    if (step < 3)
                        Assert.Less(Quaternion.Angle(Quaternion.identity, replica.localRotation), 0.1f);
                }
                Assert.Less(Quaternion.Angle(source.localRotation, replica.localRotation), 0.1f);
            }
            finally
            {
                client.Stop();
                server.Stop();
                UnityEngine.Object.DestroyImmediate(client.gameObject);
                UnityEngine.Object.DestroyImmediate(server.gameObject);
                UnityEngine.Object.DestroyImmediate(template);
                UnityEngine.Object.DestroyImmediate(list);
            }
        }

        [UnityTest] public IEnumerator WorkerOwnedDoorLateJoinUsesLatestReceivedDeltaBeforeSpawnCallback()
        {
            var template=new GameObject("Worker door template");template.SetActive(false);var prefab=template.AddComponent<NetworkObject>();Set(prefab,"prefabId","worker-door-test");
            var pose=template.AddComponent<NetworkTransform>();Set(pose,"rotationWriter",TransformWriter.Server);template.AddComponent<PersistentDoorProbe>();
            var list=ScriptableObject.CreateInstance<NetworkPrefabsList>();Set(list,"prefabs",new List<NetworkObject>{prefab});
            var server=Create("Worker door coordinator",17885);var worker=Create("Worker door authority",17885);var client=Create("Worker door observer",17885);
            foreach(var m in new[]{server,worker,client})Set(m,"networkPrefabsLists",new[]{list});Set(server,"automaticLocalHandoffs",true);
            try
            {
                server.StartServer();worker.StartWorker();
                yield return Until(()=>server.WorkerCount==2,"worker joined");
                var id=server.Spawn(prefab,new Vector3(3,0,0),Quaternion.identity).EntityId;
                yield return Until(()=>worker.SpawnedObjects.Any(o=>o.EntityId==id&&o.HasAuthority),"door handed off");
                client.StartClient();yield return Until(()=>client.SpawnedCount==1&&client.IsConnectedClient,"observer ready");
                // Stop replaceable full snapshots, leaving the reliable variable path active.
                worker.enabled=false;
                worker.SpawnedObjects.Single(o=>o.EntityId==id).GetComponent<PersistentDoorProbe>().OpenFraction.Value=.6f;
                typeof(NetworkManager).GetMethod("LateUpdate",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(worker,null);
                yield return Until(()=>client.SpawnedObjects.Single().GetComponent<PersistentDoorProbe>().OpenFraction.Value==.6f,"reliable variable arrived");
                var late=Create("Late worker door observer",17885,ClientTransport.WebSocket);
                Set(late,"networkPrefabsLists",new[]{list});
                try
                {
                    late.StartClient();yield return Until(()=>late.SpawnedCount==1&&late.IsConnectedClient,"late observer created");
                    Assert.AreEqual(.6f,late.SpawnedObjects.Single().GetComponent<PersistentDoorProbe>().InitialFraction,
                        "Latest received fields must override an older cached worker snapshot before OnNetworkSpawn.");
                }
                finally{late.Stop();UnityEngine.Object.DestroyImmediate(late.gameObject);}
            }
            finally{client.Stop();worker.Stop();server.Stop();foreach(var m in new[]{client,worker,server})UnityEngine.Object.DestroyImmediate(m.gameObject);UnityEngine.Object.DestroyImmediate(template);UnityEngine.Object.DestroyImmediate(list);}
        }
    }
}
