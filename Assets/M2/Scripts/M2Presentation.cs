using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using AtlasNet.Samples.Soccer;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AtlasNet.M2
{
    // Presentation orchestration is application code, outside the UPM package.
    public sealed class M2Presentation : MonoBehaviour
    {
        public static M2Presentation Instance { get; private set; }
        public NetworkManager manager;
        public M2Hud hud;
        public string[] scenes;
        public string webBaseUrl = "http://localhost:8094/";
        public int ActiveIndex { get; private set; } = -1;
        public int AppliedRevision { get; private set; }
        public bool Loading { get; private set; }
        public M2Stage Stage { get; private set; }
        public string Error { get; private set; } = "";
        public int ReadyCount => ready.Count;
        readonly Dictionary<SessionId,int> ready = new();
        readonly List<NetworkObject> extras = new();
        int requestedIndex, requestedRevision;
        Scene content;
        NetworkObject owner;
        bool wasRunning;
        const string StateMessage="M2.Presentation.State", HelloMessage="M2.Presentation.Hello", ReadyMessage="M2.Presentation.Ready";

        void Awake()
        {
            Instance=this; DontDestroyOnLoad(gameObject);
            manager.PlayerSpawnPosition=s=>SpawnPosition(s);
            manager.OnClientConnectedCallback+=Joined;
            manager.SessionLeft+=Left;
        }
        void OnDestroy()
        {
            manager.OnClientConnectedCallback-=Joined; manager.SessionLeft-=Left;
            if(Instance==this)Instance=null;
        }
        void Start()
        {
            if(Application.platform!=RuntimePlatform.WebGLPlayer)return;
            if(Uri.TryCreate(Application.absoluteURL,UriKind.Absolute,out var url))
            {
                hud.Address=url.Host;
                foreach(var item in url.Query.TrimStart('?').Split('&'))
                {
                    var pair=item.Split('=');if(pair.Length!=2)continue;
                    if(pair[0]=="host")hud.Address=Uri.UnescapeDataString(pair[1]);
                    if(pair[0]=="port"&&int.TryParse(pair[1],out int port))manager.ConfigureConnection(hud.Address,port,ClientTransport.WebSocket);
                }
            }
            Connect(2);
        }
        public void Connect(int role)
        {
            if(manager.IsRunning)return;
            try
            {
                Error=""; ready.Clear();requestedRevision=0;AppliedRevision=0;
                manager.CustomMessagingManager.RegisterNamedMessageHandler(StateMessage,ReceiveState);
                manager.CustomMessagingManager.RegisterNamedMessageHandler(HelloMessage,(s,r)=>{if(manager.IsServer)SendState(s);});
                manager.CustomMessagingManager.RegisterNamedMessageHandler(ReadyMessage,(s,r)=>{int revision=r.ReadInt();if(manager.IsServer&&revision==requestedRevision)ready[s]=revision;});
                manager.ConfigureConnection(hud.Address,manager.Port,Application.platform==RuntimePlatform.WebGLPlayer?ClientTransport.WebSocket:ClientTransport.Udp,ServerTransport.UdpAndWebSocket);
                if(role==0)manager.StartHost();else if(role==3)manager.StartServer();else manager.StartClient();
                if(manager.IsServer)Select(0);
            }
            catch(Exception ex){Error=ex.Message;Debug.LogException(ex,this);}
        }
        void Joined(SessionId session)
        {
            if(manager.IsServer)
            {
                if(!Loading&&Stage)SpawnPlayer(session);
                if(session!=manager.LocalSession)SendState(session);
            }
            else
            {
                using var hello=new NetWriter();manager.CustomMessagingManager.SendNamedMessage(HelloMessage,NetworkManager.ServerSessionId,hello);
            }
        }
        void Left(SessionId session)=>ready.Remove(session);
        void SendState(SessionId session)
        {
            if(requestedRevision==0)return;
            using var message=new NetWriter();message.Write(requestedIndex);message.Write(requestedRevision);
            manager.CustomMessagingManager.SendNamedMessage(StateMessage,session,message);
        }
        void ReceiveState(SessionId sender,NetReader message)
        {
            if(manager.IsServer||sender!=NetworkManager.ServerSessionId)return;
            int index=message.ReadInt(),revision=message.ReadInt();
            if(index<0||index>=scenes.Length||revision<=requestedRevision)return;
            requestedIndex=index;requestedRevision=revision;
            if(!Loading)StartCoroutine(LoadPending());
        }
        public void Select(int index)
        {
            if(!manager.IsServer||manager.IsWorker||index<0||index>=scenes.Length)return;
            requestedIndex=index;requestedRevision++;ready.Clear();
            foreach(var session in manager.ConnectedClientsIds)if(session!=manager.LocalSession)SendState(session);
            // This presentation starts a single coordinator; per-mode entities are transient, sessions are not.
            foreach(var obj in manager.SpawnedObjects.ToArray())manager.Despawn(obj);
            extras.Clear();owner=null;
            if(!Loading)StartCoroutine(LoadPending());
        }
        IEnumerator LoadPending()
        {
            Loading=true;hud.HideScreenshot();
            while(AppliedRevision!=requestedRevision)
            {
                int index=requestedIndex,revision=requestedRevision;
                if(!Stage||ActiveIndex!=index)
                {
                    var previous=content;
                    yield return SceneManager.LoadSceneAsync(scenes[index],LoadSceneMode.Additive);
                    content=SceneManager.GetSceneByPath(scenes[index]);SceneManager.SetActiveScene(content);
                    Stage=content.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<M2Stage>(true)).Single();
                    if(previous.IsValid()&&previous.isLoaded)yield return SceneManager.UnloadSceneAsync(previous);
                }
                ActiveIndex=index;AppliedRevision=revision;owner=null;
                if(Stage.goalPointer){Stage.goalPointer.manager=manager;Stage.goalPointer.enabled=true;}
                var confetti=GetComponent<SoccerGoalConfetti>();confetti.manager=manager;confetti.blueGoal=Stage.blueConfetti;confetti.orangeGoal=Stage.orangeConfetti;
                if(revision!=requestedRevision)continue;
                if(manager.IsServer)
                {
                    foreach(var session in manager.ConnectedClientsIds)SpawnPlayer(session);
                    if(index==2&&Stage.extraPrefab)extras.Add(manager.Spawn(Stage.extraPrefab,new Vector3(0,SoccerBall.Radius,0),Quaternion.identity));
                    if(manager.IsClient)ready[manager.LocalSession]=revision;
                }
                else if(manager.IsConnectedClient)
                {
                    using var message=new NetWriter();message.Write(revision);manager.CustomMessagingManager.SendNamedMessage(ReadyMessage,NetworkManager.ServerSessionId,message);
                }
            }
            Loading=false;
        }
        Vector3 SpawnPosition(SessionId session)
        {
            int index=SoccerPlayer.RosterIndex(manager,session);
            return new Vector3((index&1)==0?-15:15,0,(index/2%5-2)*3f);
        }
        void SpawnPlayer(SessionId session)
        {
            // Replace the automatic default avatar with this stage's configured avatar.
            foreach(var obj in manager.SpawnedObjects.Where(o=>o.OwnerSession==session&&o.GetComponent<SoccerPlayer>()).ToArray())manager.Despawn(obj);
            manager.Spawn(Stage.playerPrefab,SpawnPosition(session),Quaternion.identity,session);
        }
        public NetworkObject Owner
        {
            get
            {
                if(!owner||!owner.IsSpawned)owner=manager.SpawnedObjects.FirstOrDefault(o=>o.IsOwner&&o.GetComponent<SoccerPlayer>());
                return owner;
            }
        }
        void Update()
        {
            if(wasRunning&&!manager.IsRunning){ready.Clear();extras.Clear();owner=null;Error=manager.DisconnectReason;}
            wasRunning=manager.IsRunning;
            if(!manager.IsRunning||Loading||!Stage)return;
            if(manager.IsServer&&!manager.IsWorker&&ActiveIndex==0)
            {
                for(int i=0;i<4&&extras.Count!=Stage.entityCount;i++)
                {
                    if(extras.Count>Stage.entityCount){var last=extras[^1];extras.RemoveAt(extras.Count-1);if(last&&last.IsSpawned)manager.Despawn(last);}
                    else {int n=extras.Count;var p=new Vector3((n%10-4.5f)*3,1,(n/10-2)*4);var obj=manager.Spawn(Stage.extraPrefab,p,Quaternion.identity);obj.GetComponent<ScaleMotion>().SetMoving(true);extras.Add(obj);}
                }
            }
            if(UnityEngine.EventSystems.EventSystem.current&&UnityEngine.EventSystems.EventSystem.current.currentSelectedGameObject?.GetComponent<TMPro.TMP_InputField>())return;
            if(manager.IsServer)
            {
                if(Input.GetKeyDown(KeyCode.Alpha1))Select(0);if(Input.GetKeyDown(KeyCode.Alpha2))Select(1);if(Input.GetKeyDown(KeyCode.Alpha3))Select(2);
                if(Input.GetKeyDown(KeyCode.I))hud.ToggleScreenshot(0);
                if(Input.GetKeyDown(KeyCode.PageDown))hud.ToggleScreenshot(1);
                if(Input.GetKeyDown(KeyCode.PageUp))hud.ToggleScreenshot(-1);
                if(Input.GetKeyDown(KeyCode.Escape))hud.HideScreenshot();
            }
        }
        void LateUpdate()
        {
            if(!manager.IsClient||Loading||!Stage||!Stage.view||!Stage.followOwner||!Owner)return;
            Vector3 focus=Owner.transform.position;
            if(ActiveIndex==2){focus.x=Mathf.Clamp(focus.x,-35,35);focus.z=Mathf.Clamp(focus.z,-18,18);}
            Stage.view.transform.SetPositionAndRotation(new Vector3(focus.x,0,focus.z)+Stage.cameraOffset,Quaternion.Euler(Stage.cameraAngles));
        }
    }
}
