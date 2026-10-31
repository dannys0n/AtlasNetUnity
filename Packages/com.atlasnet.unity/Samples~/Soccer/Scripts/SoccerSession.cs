using System;
using System.Net;
using System.Net.NetworkInformation;
using AtlasNet;
using AtlasNet.Samples.Soccer.QrCodeGenerator;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TMPro;

namespace AtlasNet.Samples.Soccer
{
    public sealed class SoccerSession : MonoBehaviour
    {
        public static readonly Color Blue = new(.12f, .48f, 1f);
        public static readonly Color Orange = new(1f, .4f, .07f);
        public NetworkManager manager;
        public NetworkObject ballPrefab;
        [Tooltip("URL of the served Soccer Web build. Set to the LAN hostname/IP, e.g. http://192.168.1.20:8094/")]
        public string webBaseUrl = "http://localhost:8094/";
        public TMP_FontAsset font;
        TMP_Text blueScore, orangeScore, notice, joinLabel, clock;
        long clockSecond = -1;
        TMP_InputField address;
        GameObject connectionPanel, joinPanel;
        RawImage qrView;
        Texture2D qr;
        string lastLink;
        SoccerMatch match;
        bool createdBall;
        Camera view;
        NetworkObject local;

        void Awake()
        {
            Application.runInBackground = true;
            manager.PlayerSpawnPosition = s => { int index = SoccerPlayer.RosterIndex(manager, s); return new Vector3((index & 1) == 0 ? -15 : 15, 0, (index / 2 % 5 - 2) * 3f); };
            BuildHud(); view = Camera.main;
        }
        void Start()
        {
            if (Application.platform == RuntimePlatform.WebGLPlayer && Uri.TryCreate(Application.absoluteURL, UriKind.Absolute, out var uri))
            {
                foreach (var item in uri.Query.TrimStart('?').Split('&'))
                {
                    var pair = item.Split('='); if (pair.Length != 2) continue;
                    if (pair[0] == "host") address.text = Uri.UnescapeDataString(pair[1]);
                    if (pair[0] == "port" && int.TryParse(pair[1], out int p)) manager.ConfigureConnection(address.text, p, ClientTransport.WebSocket, ServerTransport.UdpAndWebSocket);
                }
                if (!uri.Query.Contains("host=")) address.text = uri.Host;
                Connect(2);
            }
        }
        public void Connect(int role)
        {
            try
            {
                manager.ConfigureConnection(address.text.Trim(), manager.Port,
                    Application.platform == RuntimePlatform.WebGLPlayer ? ClientTransport.WebSocket : manager.ClientConnection,
                    ServerTransport.UdpAndWebSocket);
                if (role == 0) manager.StartHost(); else if (role == 1) manager.StartWorker(); else if (role == 3) manager.StartServer(); else manager.StartClient();
                notice.text = "";
            }
            catch (Exception ex) { notice.text = ex.Message; }
        }
        void Update()
        {
            var now = DateTime.Now;
            long second = now.Ticks / TimeSpan.TicksPerSecond;
            if (second != clockSecond)
            {
                clockSecond = second;
                clock.text = now.ToString("HH:mm:ss");
            }
            connectionPanel.SetActive(!manager.IsRunning);
            joinPanel.SetActive(manager.IsRunning && manager.IsServer);
            if (!manager.IsRunning) { match = null; local = null; createdBall = false; return; }
            if (!match)
                foreach (var obj in manager.SpawnedObjects)
                    if (obj.TryGetComponent(out SoccerMatch found)) { match = found; break; }
            if (manager.IsServer && !manager.IsWorker && !createdBall)
            {
                match = manager.Spawn(ballPrefab, new Vector3(0, SoccerBall.Radius, 0), Quaternion.identity).GetComponent<SoccerMatch>();
                createdBall = true;
            }
            blueScore.text = match ? match.BlueScore.Value.ToString() : "0";
            orangeScore.text = match ? match.OrangeScore.Value.ToString() : "0";
            if (!manager.IsClient && manager.IsServer) notice.text = (manager.IsWorker ? "WORKER  " : "SERVER  ") + manager.LocalWorkerId + "   |   " + manager.LocalAuthorityCount + " authoritative entities";
            if (manager.IsClient && (!local || !local.IsSpawned))
                foreach (var obj in manager.SpawnedObjects) if (obj.IsOwner && obj.GetComponent<SoccerPlayer>()) { local = obj; break; }
            if (manager.IsServer) RefreshJoin();
        }
        void LateUpdate()
        {
            if (!view || !manager.IsClient || !local) return;
            Vector3 focus = local.transform.position;
            focus.x = Mathf.Clamp(focus.x, -35, 35); focus.z = Mathf.Clamp(focus.z, -18, 18);
            Vector3 desired = focus + new Vector3(0, 32, -26);
            // Follow the already-interpolated render pose; a second smoothing pass adds movement lag.
            view.transform.position = desired;
            view.transform.rotation = Quaternion.Euler(51, 0, 0);
        }
        void RefreshJoin()
        {
            string host = manager.Address;
            if (host == "localhost" || host == "127.0.0.1" || host == "0.0.0.0") host = LanAddress();
            if (!Uri.TryCreate(webBaseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")) return;
            var url = new UriBuilder(uri);
            if (url.Host == "localhost" || url.Host == "127.0.0.1") url.Host = host;
            url.Query = ""; url.Fragment = "";
            string link = url.Uri.AbsoluteUri;
            if (lastLink == link) return;
            lastLink = link; joinLabel.text = link;
            var code = QrCode.EncodeText(link, QrCode.Ecc.Medium);
            int size = code.Size + 8;
            if (qr) Destroy(qr);
            qr = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
                qr.SetPixel(x, size - 1 - y, code.GetModule(x - 4, y - 4) ? Color.black : Color.white);
            qr.Apply(); qrView.texture = qr;
        }
        static string LanAddress()
        {
#if !UNITY_WEBGL || UNITY_EDITOR
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                if (nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback && nic.GetIPProperties().GatewayAddresses.Count > 0)
                    foreach (var address in nic.GetIPProperties().UnicastAddresses)
                        if (address.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(address.Address)) return address.Address.ToString();
#endif
            return "127.0.0.1";
        }
        void OnDestroy() { if (qr) Destroy(qr); }
        void BuildHud()
        {
            if (!font) font = TMP_Settings.defaultFontAsset;
            var canvas = new GameObject("Soccer HUD", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvas.transform.SetParent(transform, false); canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var scale = canvas.GetComponent<CanvasScaler>(); scale.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scale.referenceResolution = new Vector2(1600, 900); scale.matchWidthOrHeight = .5f;
            if (!FindAnyObjectByType<EventSystem>()) new GameObject("Soccer EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            var score = Panel(canvas.transform, "Scoreboard", new Vector2(.5f, 1), new Vector2(0, -38), new Vector2(330, 62), new Color(.03f,.05f,.08f,.95f));
            blueScore = Label(score, "Blue", "0", new Vector2(-112,0), new Vector2(100,62), 38, Color.white);
            var blueBg=Panel(score,"Blue color",new Vector2(.5f,.5f),new Vector2(-112,0),new Vector2(100,62),Blue); blueBg.SetAsFirstSibling();
            orangeScore = Label(score, "Orange", "0", new Vector2(112,0), new Vector2(100,62), 38, Color.white);
            var orangeBg=Panel(score,"Orange color",new Vector2(.5f,.5f),new Vector2(112,0),new Vector2(100,62),Orange); orangeBg.SetAsFirstSibling();
            clock = Label(score,"Clock",DateTime.Now.ToString("HH:mm:ss"),Vector2.zero,new Vector2(120,62),19,Color.white);
            notice=Label(canvas.transform,"Status","",new Vector2(0,-85),new Vector2(900,45),18,Color.white); notice.rectTransform.anchorMin=notice.rectTransform.anchorMax=new Vector2(.5f,1);
            var lobby=Panel(canvas.transform,"Connection",new Vector2(.5f,.5f),Vector2.zero,new Vector2(440,370),new Color(.025f,.04f,.07f,.97f)); connectionPanel=lobby.gameObject;
            Label(lobby,"Welcome","ATLASNET SOCCER",new Vector2(0,145),new Vector2(420,40),28,Color.white);
            address=Field(lobby,"Server","127.0.0.1",new Vector2(0,85),new Vector2(380,42));
            Button(lobby,"Host",new Vector2(-100,25),new Vector2(180,44),()=>Connect(0));
            Button(lobby,"Join",new Vector2(100,25),new Vector2(180,44),()=>Connect(2));
            Button(lobby,"Join as worker",new Vector2(0,-35),new Vector2(380,40),()=>Connect(1));
            Button(lobby,"Start server only",new Vector2(0,-92),new Vector2(380,42),()=>Connect(3));
            Label(lobby,"Controls","WASD / ARROWS OR TOUCH STICK TO MOVE",new Vector2(0,-150),new Vector2(420,30),14,Color.white);
            var join=Panel(canvas.transform,"Worker join QR",new Vector2(0,1),new Vector2(245,-290),new Vector2(450,530),new Color(.025f,.04f,.07f,.95f)); joinPanel=join.gameObject;
            Label(join,"Join title","JOIN THIS SESSION",new Vector2(0,230),new Vector2(420,38),24,Color.white);
            var qrRect=Panel(join,"QR background",new Vector2(.5f,.5f),new Vector2(0,35),new Vector2(340,340),Color.white);
            var qrObject=new GameObject("QR",typeof(RectTransform),typeof(RawImage));qrObject.transform.SetParent(qrRect,false);qrView=qrObject.GetComponent<RawImage>();qrView.rectTransform.sizeDelta=new Vector2(340,340);qrView.raycastTarget=false;
            joinLabel=Label(join,"Join address","",new Vector2(0,-180),new Vector2(420,75),30,Color.white);joinLabel.textWrappingMode=TextWrappingModes.Normal;
            Button(join,"Copy address",new Vector2(0,-238),new Vector2(420,36),()=>GUIUtility.systemCopyBuffer=lastLink);
            var stick=Circle(canvas.transform,"Movement",new Vector2(0,0),new Vector2(220,210),new Vector2(220,220),new Color(.04f,.08f,.13f,.5f));
            var control=stick.gameObject.AddComponent<SoccerStick>();
            control.handle=Circle(stick,"Handle",new Vector2(.5f,.5f),Vector2.zero,new Vector2(80,80),new Color(.8f,.9f,1,.65f));control.handle.GetComponent<SoccerStickGraphic>().raycastTarget=false;
            Button(canvas.transform,"Disconnect",new Vector2(-90,-30),new Vector2(150,35),()=>manager.Stop()).GetComponent<RectTransform>().anchorMin=new Vector2(1,1);
            var stop=canvas.transform.Find("Disconnect").GetComponent<RectTransform>();stop.anchorMax=new Vector2(1,1);stop.anchoredPosition=new Vector2(-90,-30);
        }
        RectTransform Circle(Transform parent,string name,Vector2 anchor,Vector2 pos,Vector2 size,Color color)
        {
            var obj=new GameObject(name,typeof(RectTransform),typeof(SoccerStickGraphic));obj.transform.SetParent(parent,false);
            var rect=obj.GetComponent<RectTransform>();rect.anchorMin=rect.anchorMax=anchor;rect.anchoredPosition=pos;rect.sizeDelta=size;
            obj.GetComponent<SoccerStickGraphic>().color=color;return rect;
        }
        RectTransform Panel(Transform parent,string name,Vector2 anchor,Vector2 pos,Vector2 size,Color color)
        {
            var obj=new GameObject(name,typeof(RectTransform),typeof(Image));obj.transform.SetParent(parent,false);
            var rect=obj.GetComponent<RectTransform>();rect.anchorMin=rect.anchorMax=anchor;rect.anchoredPosition=pos;rect.sizeDelta=size;
            obj.GetComponent<Image>().color=color;return rect;
        }
        TMP_Text Label(Transform parent,string name,string value,Vector2 pos,Vector2 size,int fontSize,Color color)
        {
            var obj=new GameObject(name,typeof(RectTransform),typeof(TextMeshProUGUI));obj.transform.SetParent(parent,false);
            var rect=obj.GetComponent<RectTransform>();rect.sizeDelta=size;rect.anchoredPosition=pos;
            var text=obj.GetComponent<TextMeshProUGUI>();text.text=value;text.font=font;text.fontSize=fontSize;text.color=color;text.alignment=TextAlignmentOptions.Center;text.enableAutoSizing=false;text.raycastTarget=false;return text;
        }
        Button Button(Transform parent,string name,Vector2 pos,Vector2 size,UnityEngine.Events.UnityAction action)
        {
            var rect=Panel(parent,name,new Vector2(.5f,.5f),pos,size,new Color(.15f,.22f,.3f,.98f));
            var button=rect.gameObject.AddComponent<Button>();button.onClick.AddListener(action);Label(rect,"Label",name,Vector2.zero,size,18,Color.white);return button;
        }
        TMP_InputField Field(Transform parent,string name,string initial,Vector2 pos,Vector2 size)
        {
            var rect=Panel(parent,name,new Vector2(.5f,.5f),pos,size,new Color(.12f,.16f,.21f));
            var viewport=new GameObject("Text viewport",typeof(RectTransform),typeof(RectMask2D)).GetComponent<RectTransform>();viewport.SetParent(rect,false);viewport.anchorMin=Vector2.zero;viewport.anchorMax=Vector2.one;viewport.offsetMin=new Vector2(8,4);viewport.offsetMax=new Vector2(-8,-4);
            var input=rect.gameObject.AddComponent<TMP_InputField>();
            var text=Label(viewport,"Text",initial,Vector2.zero,size-new Vector2(16,8),20,Color.white);
            text.rectTransform.anchorMin=Vector2.zero;text.rectTransform.anchorMax=Vector2.one;text.rectTransform.offsetMin=Vector2.zero;text.rectTransform.offsetMax=Vector2.zero;
            text.alignment=TextAlignmentOptions.MidlineLeft;input.textViewport=viewport;input.textComponent=(TextMeshProUGUI)text;input.fontAsset=font;input.pointSize=20;input.text=initial;return input;
        }
    }
}