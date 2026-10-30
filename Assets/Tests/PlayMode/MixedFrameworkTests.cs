using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using AtlasNet;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
namespace AtlasNetDemo.Tests
{
    public sealed class TransportProbe : NetworkBehaviour
    {
        public readonly NetworkVariable<int> Value=new(0);
        public void Ping()=>ReliablePingRpc();
        public void State(int value)=>StateRpc(value);
        [Rpc(SendTo.Authority)] void ReliablePingRpc(){Value.Value++;}
        [Rpc(SendTo.Authority,Delivery=RpcDelivery.UnreliableSequenced)] void StateRpc(int value){Value.Value=value;}
    }
    public sealed class MixedFrameworkTests
    {
        static void Set(object target,string name,object value)=>target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(target,value);
        [UnityTest] public IEnumerator UdpAndWebSocketClientsShareSpawnsOwnershipAndRpcs()
        {
            var template=new GameObject("Registered probe");template.SetActive(false);
            var prefab=template.AddComponent<NetworkObject>();template.AddComponent<TransportProbe>();Set(prefab,"prefabId","mixed-framework-probe");
            var list=ScriptableObject.CreateInstance<NetworkPrefabsList>();Set(list,"prefabs",new System.Collections.Generic.List<NetworkObject>{prefab});
            NetworkManager Create(string name,ClientTransport transport)
            {
                var manager=new GameObject(name).AddComponent<NetworkManager>();Set(manager,"playerPrefab",prefab);Set(manager,"networkPrefabsLists",new[]{list});
                manager.ConfigureConnection("127.0.0.1",17854,transport);return manager;
            }
            var server=Create("Mixed server",ClientTransport.Udp);var udp=Create("UDP player",ClientTransport.Udp);var web=Create("Browser transport player",ClientTransport.WebSocket);
            try
            {
                int synchronizedClients=0;
                udp.OnClientConnectedCallback+=id=>{Assert.IsTrue(udp.SpawnedObjects.Any(o=>o.IsOwner), "Connected callback must follow initial owner replica creation.");synchronizedClients++;};
                web.OnClientConnectedCallback+=id=>{Assert.IsTrue(web.SpawnedObjects.Any(o=>o.IsOwner), "WebSocket connected callback must follow initial owner replica creation.");synchronizedClients++;};
                int approvals=0;server.ConnectionApproval=(peer,data)=>{approvals++;return data.Length==1&&data[0]==42;};
                udp.ConnectionData=web.ConnectionData=new byte[]{42};server.StartServer();udp.StartClient();web.StartClient();
                float deadline=Time.realtimeSinceStartup+15;
                while((udp.SpawnedCount!=2||web.SpawnedCount!=2||synchronizedClients!=2)&&Time.realtimeSinceStartup<deadline)yield return null;
                Assert.AreEqual(2,server.RemoteClientCount);Assert.AreEqual(2,udp.SpawnedCount);Assert.AreEqual(2,web.SpawnedCount);Assert.AreEqual(2,approvals);Assert.AreEqual(2,synchronizedClients);
                Assert.AreNotEqual(udp.LocalSession,web.LocalSession);
                var owned=udp.SpawnedObjects.Single(o=>o.IsOwner);Assert.IsFalse(web.SpawnedObjects.Single(o=>o.EntityId==owned.EntityId).IsOwner);
                owned.GetComponent<TransportProbe>().Ping();
                deadline=Time.realtimeSinceStartup+5;
                while(web.SpawnedObjects.Single(o=>o.EntityId==owned.EntityId).GetComponent<TransportProbe>().Value.Value!=1&&Time.realtimeSinceStartup<deadline)yield return null;
                Assert.AreEqual(1,web.SpawnedObjects.Single(o=>o.EntityId==owned.EntityId).GetComponent<TransportProbe>().Value.Value);
                for(int i=2;i<=8;i++){owned.GetComponent<TransportProbe>().State(i);yield return null;}
                deadline=Time.realtimeSinceStartup+5;
                while(web.SpawnedObjects.Single(o=>o.EntityId==owned.EntityId).GetComponent<TransportProbe>().Value.Value!=8&&Time.realtimeSinceStartup<deadline)yield return null;
                Assert.AreEqual(8,web.SpawnedObjects.Single(o=>o.EntityId==owned.EntityId).GetComponent<TransportProbe>().Value.Value);
                Assert.IsTrue(server.TryGet(owned.EntityId,out var canonical));
                canonical.transform.position=new Vector3(3,4,5);
                var migration=server.CaptureMigrationState(canonical.EntityId);var entity=canonical.EntityId;var epoch=canonical.AuthorityEpoch;
                server.RemoveReplica(entity);
                var restored=server.RestoreMigratedReplica(prefab,entity,udp.LocalSession,0,epoch,migration);
                Assert.AreEqual(entity,restored.EntityId);Assert.AreEqual(udp.LocalSession,restored.OwnerSession);
                Assert.AreEqual(new Vector3(3,4,5),restored.transform.position);
                Assert.AreEqual(8,restored.GetComponent<TransportProbe>().Value.Value,"Migration must restore persistent state before normal lifecycle callbacks.");
                web.Stop();deadline=Time.realtimeSinceStartup+5;
                while(server.RemoteClientCount!=1&&Time.realtimeSinceStartup<deadline)yield return null;
                Assert.AreEqual(1,server.RemoteClientCount);
            }
            finally
            {
                udp.Stop();web.Stop();server.Stop();
                UnityEngine.Object.Destroy(udp.gameObject);UnityEngine.Object.Destroy(web.gameObject);UnityEngine.Object.Destroy(server.gameObject);
                UnityEngine.Object.Destroy(template);UnityEngine.Object.Destroy(list);
            }
            yield return null;
        }
    }
}
