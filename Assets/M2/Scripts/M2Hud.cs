using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using AtlasNet.Samples.Soccer;
using AtlasNet.Samples.Soccer.QrCodeGenerator;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace AtlasNet.M2
{
    public sealed class M2Hud : MonoBehaviour
    {
        public M2Presentation presentation;
        public TMP_FontAsset font;
        public Material videoTextBlurMaterial;
        TMP_InputField address;
        TMP_Text status,linkLabel,score;
        GameObject connection,join,stick,waveButton;
        M2Slides slides;
        RectTransform gameHud, fixedHud, joinHud;
        int waveStage = -1;
        UnityEngine.UI.RawImage qrView;
        Texture2D qr;
        string link;
        string wifiAddress, ethernetAddress, fallbackAddress;
        bool preferWifi=true;
        float nextAddressScan;
        UnityEngine.UI.Button networkButton;
        TMP_Text networkLabel;
        public bool HasPartialSlide => slides && slides.Partial;
        public string Address { get=>address?address.text:"127.0.0.1";set{if(address)address.text=value;} }
        void Awake()=>Build();
        void OnDestroy(){if(qr)Destroy(qr);}
        void Update()
        {
            var p=presentation;var m=p.manager;bool running=m.IsRunning;
            connection.SetActive(!running);join.SetActive(running&&m.IsServer);
            stick.SetActive(running&&m.IsClient&&!p.Loading&&p.ActiveKind==M2StageKind.Soccer&&p.Owner);
            waveButton.SetActive(running&&m.IsClient&&!p.Loading&&(p.ActiveKind==M2StageKind.AnimatorSync||p.ActiveKind==M2StageKind.Soccer)&&p.Owner);
            if (waveStage != p.ActiveIndex)
            {
                waveStage = p.ActiveIndex;
                var rect = waveButton.GetComponent<RectTransform>();
                rect.SetParent(p.ActiveKind == M2StageKind.Soccer ? fixedHud : gameHud, false);
                Anchor(rect, new Vector2(p.ActiveKind == M2StageKind.Soccer ? 1 : .5f, 0));
                rect.anchoredPosition = new Vector2(p.ActiveKind == M2StageKind.Soccer ? -210 : 0, 90);
            }
            status.text=!running?(string.IsNullOrEmpty(p.Error)?"ATLASNET - MILESTONE 2":p.Error):p.Loading?"LOADING DEMO...":!m.IsServer&&!m.IsConnectedClient?"CONNECTING-":$"M2 - {p.Stage?.title ?? "Preparing"} - {m.ConnectedClientsIds.Count} {((p.ActiveKind==M2StageKind.Install||p.ActiveKind==M2StageKind.InterestAndScale)?"viewers":"players")}";
            score.text="";
            if(running&&p.ActiveKind==M2StageKind.Soccer)
            {
                var match=m.SpawnedObjects.Select(o=>o.GetComponent<SoccerMatch>()).FirstOrDefault(o=>o);
                if(match)score.text=$"<color=#3C9BFF>{match.BlueScore.Value}</color>    {DateTime.Now:HH:mm:ss}    <color=#FF781E>{match.OrangeScore.Value}</color>";
            }
            if(running&&m.IsServer)RefreshQr();
        }
        void RefreshQr()
        {
            if(Time.unscaledTime>=nextAddressScan) ScanAddresses();
            string selected=preferWifi?wifiAddress:ethernetAddress;
            if(string.IsNullOrEmpty(selected))selected=wifiAddress??ethernetAddress;
            networkButton.interactable=!string.IsNullOrEmpty(wifiAddress)&&!string.IsNullOrEmpty(ethernetAddress);
            networkLabel.text=selected==wifiAddress&&!string.IsNullOrEmpty(selected)?"Wi-Fi IP":"Ethernet IP";
            string host=selected??presentation.manager.Address;
            if(host is "127.0.0.1" or "localhost" or "0.0.0.0")host=fallbackAddress??"127.0.0.1";
            var url=new UriBuilder(presentation.webBaseUrl);if(!string.IsNullOrEmpty(selected)||url.Host is "127.0.0.1" or "localhost")url.Host=host;
            url.Query="";url.Fragment="";string next=url.Uri.AbsoluteUri;if(next==link)return;link=next;linkLabel.text=link;
            var code=QrCode.EncodeText(link,QrCode.Ecc.Medium);int size=code.Size+8;
            if(qr)Destroy(qr);qr=new Texture2D(size,size,TextureFormat.RGBA32,false){filterMode=FilterMode.Point};
            for(int y=0;y<size;y++)for(int x=0;x<size;x++)qr.SetPixel(x,size-1-y,code.GetModule(x-4,y-4)?Color.black:Color.white);
            qr.Apply();qrView.texture=qr;
        }
        void ScanAddresses()
        {
            nextAddressScan=Time.unscaledTime+5;
            wifiAddress=ethernetAddress=fallbackAddress=null;
#if !UNITY_WEBGL || UNITY_EDITOR
            try
            {
                foreach(var nic in NetworkInterface.GetAllNetworkInterfaces().OrderBy(n=>n.Id))
                {
                    if(nic.OperationalStatus!=OperationalStatus.Up||nic.NetworkInterfaceType==NetworkInterfaceType.Loopback)continue;
                    var properties=nic.GetIPProperties();
                    if(!properties.GatewayAddresses.Any(g=>g.Address.AddressFamily==AddressFamily.InterNetwork&&!g.Address.Equals(IPAddress.Any)))continue;
                    foreach(var entry in properties.UnicastAddresses)
                    {
                        var ip=entry.Address;
                        if(ip.AddressFamily!=AddressFamily.InterNetwork||IPAddress.IsLoopback(ip)||ip.ToString().StartsWith("169.254."))continue;
                        fallbackAddress??=ip.ToString();
                        if(nic.NetworkInterfaceType==NetworkInterfaceType.Wireless80211)wifiAddress??=ip.ToString();
                        else if(nic.NetworkInterfaceType==NetworkInterfaceType.Ethernet||nic.NetworkInterfaceType==NetworkInterfaceType.GigabitEthernet)ethernetAddress??=ip.ToString();
                    }
                }
            }
            catch(NetworkInformationException) { }
#endif
        }
        public void SetPresentationViewport(Rect viewport)
        {
            if (!gameHud) return;
            gameHud.anchorMin = viewport.min; gameHud.anchorMax = viewport.max;
            gameHud.offsetMin = gameHud.offsetMax = Vector2.zero;
            if (joinHud)
            {
                joinHud.anchorMin = viewport.min; joinHud.anchorMax = viewport.max;
                joinHud.offsetMin = joinHud.offsetMax = Vector2.zero;
            }
        }
        public int SlideCount => slides ? slides.SlideCount : 0;
        public void HideScreenshot() => presentation.CloseSlides();
        public void ToggleScreenshot(int direction) => presentation.ChangeSlide(direction);
        public void CloseSlideLocally() { if (slides) slides.Hide(); }
        public void ApplySlideState(int index, double elapsed) { if (slides) slides.Apply(index, elapsed); }
        void Build()
        {
            if(!font)font=TMP_Settings.defaultFontAsset;
            var canvas=new GameObject("M2 HUD",typeof(Canvas),typeof(UnityEngine.UI.CanvasScaler),typeof(UnityEngine.UI.GraphicRaycaster));canvas.transform.SetParent(transform,false);canvas.GetComponent<Canvas>().renderMode=RenderMode.ScreenSpaceOverlay;
            var scaler=canvas.GetComponent<UnityEngine.UI.CanvasScaler>();scaler.uiScaleMode=UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;scaler.referenceResolution=new Vector2(1600,900);scaler.matchWidthOrHeight=.5f;
            gameHud = new GameObject("Game HUD area", typeof(RectTransform)).GetComponent<RectTransform>();
            gameHud.SetParent(canvas.transform, false);
            SetPresentationViewport(new Rect(0, 0, 1, 1));
            fixedHud = new GameObject("Fixed screen controls", typeof(RectTransform)).GetComponent<RectTransform>();
            fixedHud.SetParent(canvas.transform, false);
            fixedHud.anchorMin = Vector2.zero; fixedHud.anchorMax = Vector2.one;
            fixedHud.offsetMin = fixedHud.offsetMax = Vector2.zero;
            joinHud = new GameObject("Join information area", typeof(RectTransform)).GetComponent<RectTransform>();
            joinHud.SetParent(canvas.transform, false);
            SetPresentationViewport(new Rect(0, 0, 1, 1));
            if(!FindAnyObjectByType<EventSystem>()){var events=new GameObject("M2 EventSystem",typeof(EventSystem),typeof(StandaloneInputModule));events.transform.SetParent(transform,false);}
            status=Text(gameHud,"Status","M2",new Vector2(0,-36),new Vector2(850,48),25);Anchor(status.rectTransform,new Vector2(.5f,1));
            score=Text(gameHud,"Score","",new Vector2(0,-84),new Vector2(440,48),28);Anchor(score.rectTransform,new Vector2(.5f,1));
            var lobby=Panel(gameHud,"Connection",new Vector2(.5f,.5f),Vector2.zero,new Vector2(450,280));connection=lobby.gameObject;
            Text(lobby,"Title","ATLASNET - M2",new Vector2(0,105),new Vector2(420,42),28);
            address=Field(lobby,new Vector2(0,45),new Vector2(400,44));
            Button(lobby,"Host",new Vector2(-105,-18),new Vector2(190,44),()=>presentation.Connect(0));
            Button(lobby,"Join",new Vector2(105,-18),new Vector2(190,44),()=>presentation.Connect(2));
            Button(lobby,"Start server only",new Vector2(0,-79),new Vector2(400,42),()=>presentation.Connect(3));
            var share=Panel(joinHud,"Join QR",new Vector2(1,1),new Vector2(-170,-235),new Vector2(310,370));join=share.gameObject;
            Text(share,"Join title","JOIN THIS SESSION",new Vector2(0,155),new Vector2(290,32),21);
            var qrObject=new GameObject("QR",typeof(RectTransform),typeof(UnityEngine.UI.RawImage));qrObject.transform.SetParent(share,false);qrView=qrObject.GetComponent<UnityEngine.UI.RawImage>();qrView.color=Color.white;qrView.rectTransform.sizeDelta=new Vector2(235,235);qrView.rectTransform.anchoredPosition=new Vector2(0,16);qrView.raycastTarget=false;
            linkLabel=Text(share,"Address","",new Vector2(0,-126),new Vector2(290,48),20);linkLabel.textWrappingMode=TextWrappingModes.Normal;
            Button(share,"Copy address",new Vector2(-74,-164),new Vector2(138,30),()=>GUIUtility.systemCopyBuffer=link);
            networkButton=Button(share,"Wi-Fi IP",new Vector2(74,-164),new Vector2(138,30),()=>{preferWifi=!preferWifi;RefreshQr();});
            networkLabel=networkButton.GetComponentInChildren<TMP_Text>();
            var circle=new GameObject("Movement",typeof(RectTransform),typeof(SoccerStickGraphic));circle.transform.SetParent(fixedHud,false);var rect=circle.GetComponent<RectTransform>();Anchor(rect,Vector2.zero);rect.anchoredPosition=new Vector2(220,210);rect.sizeDelta=new Vector2(220,220);circle.GetComponent<SoccerStickGraphic>().color=new Color(.1f,.2f,.3f,.65f);stick=circle;
            var knob=new GameObject("Handle",typeof(RectTransform),typeof(SoccerStickGraphic));knob.transform.SetParent(rect,false);var handle=knob.GetComponent<RectTransform>();handle.sizeDelta=new Vector2(80,80);knob.GetComponent<SoccerStickGraphic>().color=new Color(.7f,.85f,1,.9f);knob.GetComponent<SoccerStickGraphic>().raycastTarget=false;circle.AddComponent<SoccerStick>().handle=handle;
            var wave=Button(gameHud,"Wave Button",new Vector2(0,90),new Vector2(360,100),()=>{var owner=presentation.Owner;if(owner)owner.GetComponent<M2Actor>().Wave();});Anchor(wave.GetComponent<RectTransform>(),new Vector2(.5f,0));wave.GetComponentInChildren<TMP_Text>().fontSize=28;waveButton=wave.gameObject;
            slides = gameObject.AddComponent<M2Slides>();
            slides.Initialize(presentation, canvas.transform, font, videoTextBlurMaterial);
            // Join information and Soccer input remain above the presentation media.
            fixedHud.SetAsLastSibling();
            joinHud.SetAsLastSibling();
        }
        static void Anchor(RectTransform r,Vector2 anchor){r.anchorMin=r.anchorMax=anchor;}
        RectTransform Panel(Transform parent,string name,Vector2 anchor,Vector2 pos,Vector2 size)
        {
            var obj=new GameObject(name,typeof(RectTransform),typeof(UnityEngine.UI.Image));obj.transform.SetParent(parent,false);var rect=obj.GetComponent<RectTransform>();Anchor(rect,anchor);rect.anchoredPosition=pos;rect.sizeDelta=size;obj.GetComponent<UnityEngine.UI.Image>().color=new Color(.025f,.04f,.065f,.97f);return rect;
        }
        TMP_Text Text(Transform parent,string name,string value,Vector2 pos,Vector2 size,int fontSize)
        {
            var obj=new GameObject(name,typeof(RectTransform),typeof(TextMeshProUGUI));obj.transform.SetParent(parent,false);var text=obj.GetComponent<TextMeshProUGUI>();text.rectTransform.anchoredPosition=pos;text.rectTransform.sizeDelta=size;text.text=value;text.font=font;text.fontSize=fontSize;text.color=Color.white;text.alignment=TextAlignmentOptions.Center;text.raycastTarget=false;return text;
        }
        UnityEngine.UI.Button Button(Transform parent,string label,Vector2 pos,Vector2 size,UnityEngine.Events.UnityAction action)
        {
            var rect=Panel(parent,label,new Vector2(.5f,.5f),pos,size);rect.GetComponent<UnityEngine.UI.Image>().color=new Color(.15f,.22f,.3f);var button=rect.gameObject.AddComponent<UnityEngine.UI.Button>();button.onClick.AddListener(action);Text(rect,"Label",label,Vector2.zero,size,18);return button;
        }
        TMP_InputField Field(Transform parent,Vector2 pos,Vector2 size)
        {
            var rect=Panel(parent,"Server address",new Vector2(.5f,.5f),pos,size);var viewport=new GameObject("Viewport",typeof(RectTransform),typeof(UnityEngine.UI.RectMask2D)).GetComponent<RectTransform>();viewport.SetParent(rect,false);viewport.anchorMin=Vector2.zero;viewport.anchorMax=Vector2.one;viewport.offsetMin=new Vector2(8,4);viewport.offsetMax=new Vector2(-8,-4);var text=Text(viewport,"Text","127.0.0.1",Vector2.zero,size,20);text.alignment=TextAlignmentOptions.MidlineLeft;text.rectTransform.anchorMin=Vector2.zero;text.rectTransform.anchorMax=Vector2.one;text.rectTransform.offsetMin=text.rectTransform.offsetMax=Vector2.zero;var field=rect.gameObject.AddComponent<TMP_InputField>();field.textViewport=viewport;field.textComponent=(TextMeshProUGUI)text;field.fontAsset=font;field.text="127.0.0.1";return field;
        }
    }
}
