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
        readonly List<EntityId> extras = new();
        readonly HashSet<EntityId> stageEntities = new();
        int requestedIndex, requestedRevision;
        int slideIndex = -1, slideRevision;
        double slideStarted;
        public int SlideIndex => slideIndex;
        public double SlideElapsed => Math.Max(0, Time.unscaledTimeAsDouble - slideStarted);
        Scene content;
        NetworkObject owner;
        NetworkObject cameraTarget, animatorPlaceholder;
        bool enterFromEnd;
        public M2StageKind ActiveKind => Stage ? Stage.kind : M2StageKind.Install;
        double nextCameraPick;
        bool wasRunning;
        const string StateMessage="M2.Presentation.State", HelloMessage="M2.Presentation.Hello", ReadyMessage="M2.Presentation.Ready";

        void Awake()
        {
            Instance=this; DontDestroyOnLoad(gameObject);
            manager.ShowViewerInterestForDebug=false;
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
                Error=""; ready.Clear();requestedRevision=0;AppliedRevision=0;slideIndex=-1;slideRevision=0;
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
            message.Write(slideIndex);message.Write(slideRevision);message.Write((float)SlideElapsed);
            manager.CustomMessagingManager.SendNamedMessage(StateMessage,session,message);
        }
        void ReceiveState(SessionId sender,NetReader message)
        {
            if(manager.IsServer||sender!=NetworkManager.ServerSessionId)return;
            if(message.Remaining!=20) { Error="Presentation versions differ. Rebuild host and clients.";return; }
            int index=message.ReadInt(),revision=message.ReadInt();
            int nextSlide=message.ReadInt(),nextSlideRevision=message.ReadInt();
            float elapsed=message.ReadFloat();
            if(index<0||index>=scenes.Length||revision<requestedRevision||revision<=0||
                nextSlide < -1||nextSlide>4096||nextSlideRevision<0||!float.IsFinite(elapsed)||elapsed<0)return;
            bool sceneChanged=revision>requestedRevision;
            if(!sceneChanged&&nextSlideRevision<=slideRevision)return;
            requestedIndex=index;requestedRevision=revision;
            slideIndex=nextSlide;slideRevision=nextSlideRevision;
            slideStarted=Time.unscaledTimeAsDouble-elapsed;
            if(sceneChanged)
            {
                if(!Loading)StartCoroutine(LoadPending());
            }
            else if(!Loading)hud.ApplySlideState(slideIndex,SlideElapsed);
        }

        void BroadcastState()
        {
            foreach(var session in manager.ConnectedClientsIds)
                if(session!=manager.LocalSession)SendState(session);
        }
        public void ChangeSlide(int direction)
        {
            if(direction!=0){AdvancePresentation(direction);return;}
            if(!CanPresent)return;
            if(slideIndex>=0)CloseSlides();else if(hud.SlideCount>0)ShowSlide(0);
        }
        bool CanPresent => manager.IsServer&&!manager.IsWorker&&!Loading&&Stage;
        void ShowSlide(int index)
        {
            slideIndex=index;slideRevision++;slideStarted=Time.unscaledTimeAsDouble;
            hud.ApplySlideState(slideIndex,0);BroadcastState();
        }
        public void AdvancePresentation(int direction)
        {
            if(!CanPresent||direction==0)return;
            int count=hud.SlideCount;
            if(Stage.slidesAfterDemo&&Stage.hasLiveDemo)
            {
                if(direction>0)
                {
                    if(slideIndex<count-1)ShowSlide(slideIndex+1);
                    else if(ActiveIndex<scenes.Length-1)Select(ActiveIndex+1);
                }
                else if(slideIndex>=0)
                {
                    if(slideIndex==0)CloseSlides();else ShowSlide(slideIndex-1);
                }
                else if(ActiveIndex>0)Select(ActiveIndex-1,true);
                return;
            }
            if(direction>0)
            {
                if(slideIndex>=0&&slideIndex<count-1)ShowSlide(slideIndex+1);
                else if(slideIndex>=0&&Stage.hasLiveDemo)CloseSlides();
                else if(ActiveIndex<scenes.Length-1)Select(ActiveIndex+1);
            }
            else
            {
                if(slideIndex>0)ShowSlide(slideIndex-1);
                else if(slideIndex<0&&count>0)ShowSlide(count-1);
                else if(ActiveIndex>0)Select(ActiveIndex-1,true);
            }
        }
        public void CloseSlides()
        {
            if(!manager.IsServer||manager.IsWorker||slideIndex<0)return;
            slideIndex=-1;slideRevision++;slideStarted=Time.unscaledTimeAsDouble;
            hud.CloseSlideLocally();BroadcastState();
        }

        public void Select(int index, bool fromEnd=false)
        {
            if(!manager.IsServer||manager.IsWorker||index<0||index>=scenes.Length)return;
            requestedIndex=index;requestedRevision++;ready.Clear();
            enterFromEnd=fromEnd;slideIndex=fromEnd?-1:0;slideRevision=0;slideStarted=Time.unscaledTimeAsDouble;
            BroadcastState();
            // IDs survive interest eviction and worker handoff; Unity references do not.
            foreach(var id in stageEntities)manager.TryDespawn(id);
            stageEntities.Clear();
            foreach(var obj in manager.SpawnedObjects.ToArray())manager.Despawn(obj);
            extras.Clear();owner=null;
            if(!Loading)StartCoroutine(LoadPending());
        }
        IEnumerator LoadPending()
        {
            Loading=true;hud.CloseSlideLocally();
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
                ActiveIndex=index;AppliedRevision=revision;owner=null;cameraTarget=null;animatorPlaceholder=null;nextCameraPick=0;
                if(Stage.goalPointer){Stage.goalPointer.manager=manager;Stage.goalPointer.enabled=true;}
                var confetti=GetComponent<SoccerGoalConfetti>();confetti.manager=manager;confetti.blueGoal=Stage.blueConfetti;confetti.orangeGoal=Stage.orangeConfetti;
                if(revision!=requestedRevision)continue;
                if(manager.IsServer)
                {
                    int resolved=enterFromEnd
                        ? (Stage.hasLiveDemo&&!Stage.slidesAfterDemo ? -1 : hud.SlideCount-1)
                        : (Stage.slidesAfterDemo||hud.SlideCount==0 ? -1 : slideIndex);
                    enterFromEnd=false;
                    if(resolved!=slideIndex){slideIndex=resolved;slideRevision++;BroadcastState();}
                }
                if(manager.IsServer)
                {
                    foreach(var session in manager.ConnectedClientsIds)SpawnPlayer(session);
                    if(Stage.kind==M2StageKind.Soccer&&Stage.extraPrefab)extras.Add(Track(manager.Spawn(Stage.extraPrefab,new Vector3(0,SoccerBall.Radius,0),Quaternion.identity)).EntityId);
                    if(manager.IsClient)ready[manager.LocalSession]=revision;
                }
                else if(manager.IsConnectedClient)
                {
                    using var message=new NetWriter();message.Write(revision);manager.CustomMessagingManager.SendNamedMessage(ReadyMessage,NetworkManager.ServerSessionId,message);
                }
            }
            Loading=false;
            hud.ApplySlideState(slideIndex,SlideElapsed);
        }
        Vector3 SpawnPosition(SessionId session)
        {
            if(ActiveKind==M2StageKind.Install||ActiveKind==M2StageKind.InterestAndScale)return Vector3.zero;
            int index=SoccerPlayer.RosterIndex(manager,session);
            if(ActiveKind==M2StageKind.AnimatorSync)return new Vector3(index*3.5f,0,0);
            return new Vector3((index&1)==0?-15:15,0,(index/2%5-2)*3f);
        }
        void SpawnPlayer(SessionId session)
        {
            // The default network prefab is an invisible observer. Reuse it in the
            // scale demo; replace it only when this stage actually needs a character.
            var owned=manager.SpawnedObjects.Where(o=>o.OwnerSession==session).ToArray();
            var existing=owned.FirstOrDefault(o=>o.PrefabId==Stage.playerPrefab.PrefabId);
            foreach(var obj in owned)if(obj!=existing)manager.Despawn(obj);
            Track(existing ? existing : manager.Spawn(Stage.playerPrefab,SpawnPosition(session),Quaternion.identity,session));
        }
        NetworkObject Track(NetworkObject obj) { stageEntities.Add(obj.EntityId); return obj; }
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
            if(wasRunning&&!manager.IsRunning){ready.Clear();extras.Clear();stageEntities.Clear();owner=null;slideIndex=-1;slideRevision=0;hud.CloseSlideLocally();Error=manager.DisconnectReason;}
            wasRunning=manager.IsRunning;
            if(!manager.IsRunning||Loading||!Stage)return;
            EnsureAnimatorPlaceholder();
            if(manager.IsServer&&!manager.IsWorker&&ActiveKind==M2StageKind.InterestAndScale)
            {
                for(int i=0;i<4&&extras.Count!=Stage.entityCount;i++)
                {
                    if(extras.Count>Stage.entityCount){var last=extras[^1];extras.RemoveAt(extras.Count-1);manager.TryDespawn(last);stageEntities.Remove(last);}
                    else {int n=extras.Count;var p=new Vector3((n%10-4.5f)*3,1,(n/10-2)*4);var obj=Track(manager.Spawn(Stage.extraPrefab,p,Quaternion.identity));obj.GetComponent<ScaleMotion>().SetMoving(true);extras.Add(obj.EntityId);}
                }
            }
            if(UnityEngine.EventSystems.EventSystem.current&&UnityEngine.EventSystems.EventSystem.current.currentSelectedGameObject?.GetComponent<TMPro.TMP_InputField>())return;
            if(manager.IsServer)
            {
                if(Input.GetKeyDown(KeyCode.Alpha1))Select(0);if(Input.GetKeyDown(KeyCode.Alpha2))Select(1);if(Input.GetKeyDown(KeyCode.Alpha3))Select(2);if(Input.GetKeyDown(KeyCode.Alpha4))Select(3);if(Input.GetKeyDown(KeyCode.Alpha5))Select(4);
                if(Input.GetKeyDown(KeyCode.I))hud.ToggleScreenshot(0);
                bool space=Input.GetKeyDown(KeyCode.Space),shift=Input.GetKey(KeyCode.LeftShift)||Input.GetKey(KeyCode.RightShift);
                if(Input.GetKeyDown(KeyCode.LeftArrow)||Input.GetKeyDown(KeyCode.UpArrow)||Input.GetKeyDown(KeyCode.PageUp)||(space&&shift))AdvancePresentation(-1);
                else if(Input.GetKeyDown(KeyCode.RightArrow)||Input.GetKeyDown(KeyCode.DownArrow)||Input.GetKeyDown(KeyCode.PageDown)||(space&&!shift))AdvancePresentation(1);
                if(Input.GetKeyDown(KeyCode.Escape))hud.HideScreenshot();
            }
        }
        void EnsureAnimatorPlaceholder()
        {
            if(!manager.IsServer||manager.IsWorker||ActiveKind!=M2StageKind.AnimatorSync)return;
            bool viewers=manager.ConnectedClientsIds.Any(s=>s!=manager.LocalSession);
            if(viewers&&animatorPlaceholder)
            {
                var id=animatorPlaceholder.EntityId;manager.TryDespawn(id);stageEntities.Remove(id);animatorPlaceholder=null;
            }
            else if(!viewers&&!Owner&&!animatorPlaceholder)
                animatorPlaceholder=Track(manager.Spawn(Stage.playerPrefab,Vector3.zero,Quaternion.identity));
        }
        NetworkObject PresenterCameraTarget()
        {
            bool valid=cameraTarget&&cameraTarget.IsSpawned&&(cameraTarget==animatorPlaceholder||manager.ConnectedClientsIds.Contains(cameraTarget.OwnerSession));
            if(ActiveKind==M2StageKind.AnimatorSync)
            {
                // Finish watching the current gesture before considering another player.
                if(valid&&cameraTarget.TryGetComponent<SampleWaveLayer>(out var currentWave)&&currentWave.IsBusy)
                    return cameraTarget;
                foreach(var candidate in manager.SpawnedObjects)
                {
                    if(!candidate||!candidate.IsSpawned||candidate==cameraTarget||
                        !manager.ConnectedClientsIds.Contains(candidate.OwnerSession)||
                        !candidate.TryGetComponent<SampleWaveLayer>(out var wave)||!wave.IsBusy)continue;
                    cameraTarget=candidate;
                    nextCameraPick=Time.unscaledTimeAsDouble+5;
                    return cameraTarget;
                }
            }
            if(valid&&Time.unscaledTimeAsDouble<nextCameraPick)return cameraTarget;
            NetworkObject selected=null;
            int count=0;
            // Reservoir sampling avoids a temporary candidate list every cycle.
            foreach(var candidate in manager.SpawnedObjects)
            {
                if(!candidate||!candidate.IsSpawned||candidate.OwnerSession.Value==0||
                    candidate.OwnerSession==manager.LocalSession||!candidate.GetComponent<SoccerPlayer>()||
                    !manager.ConnectedClientsIds.Contains(candidate.OwnerSession)||
                    (valid&&candidate.OwnerSession==cameraTarget.OwnerSession))continue;
                if(UnityEngine.Random.Range(0,++count)==0)selected=candidate;
            }
            cameraTarget=selected ? selected : valid ? cameraTarget : Owner ? Owner : animatorPlaceholder;
            nextCameraPick=Time.unscaledTimeAsDouble+5;
            return cameraTarget;
        }
        void LateUpdate()
        {
            if(!manager.IsRunning||Loading||!Stage||!Stage.view||!Stage.followOwner)return;
            var target=manager.IsServer&&!manager.IsWorker ? PresenterCameraTarget() : Owner;
            if(!target)return;
            Vector3 focus=target.transform.position;
            if(ActiveKind==M2StageKind.Soccer&&!hud.HasPartialSlide)
                SoccerCameraFraming.Follow(Stage.view, focus, Stage.cameraOffset, Quaternion.Euler(Stage.cameraAngles));
            else Stage.view.transform.SetPositionAndRotation(new Vector3(focus.x,0,focus.z)+Stage.cameraOffset,Quaternion.Euler(Stage.cameraAngles));
        }
    }
}
