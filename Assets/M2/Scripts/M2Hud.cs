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
        TMP_InputField address;
        TMP_Text status,linkLabel,score;
        GameObject connection,join,controls,stick,slide;
        UnityEngine.UI.RawImage qrView,slideView;
        UnityEngine.UI.AspectRatioFitter slideAspect;
        Texture2D qr;
        string link;
        int slideIndex;
        public string Address { get=>address?address.text:"127.0.0.1";set{if(address)address.text=value;} }
        void Awake()=>Build();
        void OnDestroy(){if(qr)Destroy(qr);}
        void Update()
        {
            var p=presentation;var m=p.manager;bool running=m.IsRunning;
            connection.SetActive(!running);join.SetActive(running&&m.IsServer);controls.SetActive(running&&m.IsServer);
            stick.SetActive(running&&m.IsClient&&!p.Loading&&p.Owner);
            status.text=!running?(string.IsNullOrEmpty(p.Error)?"ATLASNET · MILESTONE 2":p.Error):p.Loading?"LOADING DEMO…":!m.IsServer&&!m.IsConnectedClient?"CONNECTING…":$"M2 · {p.Stage?.title ?? "Preparing"} · {m.ConnectedClientsIds.Count} players";
            score.text="";
            if(running&&p.ActiveIndex==2)
            {
                var match=m.SpawnedObjects.Select(o=>o.GetComponent<SoccerMatch>()).FirstOrDefault(o=>o);
                if(match)score.text=$"<color=#3C9BFF>{match.BlueScore.Value}</color>    {DateTime.Now:HH:mm:ss}    <color=#FF781E>{match.OrangeScore.Value}</color>";
            }
            if(running&&m.IsServer)RefreshQr();
        }
        void RefreshQr()
        {
            string host=presentation.manager.Address;
            if(host is "127.0.0.1" or "localhost" or "0.0.0.0")host=LanAddress();
            var url=new UriBuilder(presentation.webBaseUrl);if(url.Host is "127.0.0.1" or "localhost")url.Host=host;
            url.Query="";url.Fragment="";string next=url.Uri.AbsoluteUri;if(next==link)return;link=next;linkLabel.text=link;
            var code=QrCode.EncodeText(link,QrCode.Ecc.Medium);int size=code.Size+8;
            if(qr)Destroy(qr);qr=new Texture2D(size,size,TextureFormat.RGBA32,false){filterMode=FilterMode.Point};
            for(int y=0;y<size;y++)for(int x=0;x<size;x++)qr.SetPixel(x,size-1-y,code.GetModule(x-4,y-4)?Color.black:Color.white);
            qr.Apply();qrView.texture=qr;
        }
        static string LanAddress()
        {
#if !UNITY_WEBGL || UNITY_EDITOR
            foreach(var nic in NetworkInterface.GetAllNetworkInterfaces())
                if(nic.OperationalStatus==OperationalStatus.Up&&nic.NetworkInterfaceType!=NetworkInterfaceType.Loopback&&nic.GetIPProperties().GatewayAddresses.Count>0)
                    foreach(var a in nic.GetIPProperties().UnicastAddresses)
                        if(a.Address.AddressFamily==AddressFamily.InterNetwork&&!IPAddress.IsLoopback(a.Address))return a.Address.ToString();
#endif
            return "127.0.0.1";
        }
        public void HideScreenshot(){if(slide)slide.SetActive(false);}
        public void ToggleScreenshot(int direction)
        {
            var images=presentation.Stage?presentation.Stage.screenshots:null;
            if(images==null||images.Length==0)return;
            if(direction==0&&slide.activeSelf){HideScreenshot();return;}
            slideIndex=(slideIndex+direction+images.Length)%images.Length;
            var image=images[slideIndex];if(!image)return;
            slideView.texture=image;slideAspect.aspectRatio=(float)image.width/image.height;slide.SetActive(true);
        }
        void Build()
        {
            if(!font)font=TMP_Settings.defaultFontAsset;
            var canvas=new GameObject("M2 HUD",typeof(Canvas),typeof(UnityEngine.UI.CanvasScaler),typeof(UnityEngine.UI.GraphicRaycaster));canvas.transform.SetParent(transform,false);canvas.GetComponent<Canvas>().renderMode=RenderMode.ScreenSpaceOverlay;
            var scaler=canvas.GetComponent<UnityEngine.UI.CanvasScaler>();scaler.uiScaleMode=UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;scaler.referenceResolution=new Vector2(1600,900);scaler.matchWidthOrHeight=.5f;
            if(!FindAnyObjectByType<EventSystem>()){var events=new GameObject("M2 EventSystem",typeof(EventSystem),typeof(StandaloneInputModule));events.transform.SetParent(transform,false);}
            status=Text(canvas.transform,"Status","M2",new Vector2(0,-36),new Vector2(850,48),25);Anchor(status.rectTransform,new Vector2(.5f,1));
            score=Text(canvas.transform,"Score","",new Vector2(0,-84),new Vector2(440,48),28);Anchor(score.rectTransform,new Vector2(.5f,1));
            var lobby=Panel(canvas.transform,"Connection",new Vector2(.5f,.5f),Vector2.zero,new Vector2(450,280));connection=lobby.gameObject;
            Text(lobby,"Title","ATLASNET · M2",new Vector2(0,105),new Vector2(420,42),28);
            address=Field(lobby,new Vector2(0,45),new Vector2(400,44));
            Button(lobby,"Host",new Vector2(-105,-18),new Vector2(190,44),()=>presentation.Connect(0));
            Button(lobby,"Join",new Vector2(105,-18),new Vector2(190,44),()=>presentation.Connect(2));
            Button(lobby,"Start server only",new Vector2(0,-79),new Vector2(400,42),()=>presentation.Connect(3));
            var share=Panel(canvas.transform,"Join QR",new Vector2(0,1),new Vector2(170,-235),new Vector2(310,370));join=share.gameObject;
            Text(share,"Join title","JOIN THIS SESSION",new Vector2(0,155),new Vector2(290,32),21);
            var qrObject=new GameObject("QR",typeof(RectTransform),typeof(UnityEngine.UI.RawImage));qrObject.transform.SetParent(share,false);qrView=qrObject.GetComponent<UnityEngine.UI.RawImage>();qrView.color=Color.white;qrView.rectTransform.sizeDelta=new Vector2(235,235);qrView.rectTransform.anchoredPosition=new Vector2(0,16);qrView.raycastTarget=false;
            linkLabel=Text(share,"Address","",new Vector2(0,-126),new Vector2(290,48),20);linkLabel.textWrappingMode=TextWrappingModes.Normal;
            Button(share,"Copy address",new Vector2(0,-164),new Vector2(280,30),()=>GUIUtility.systemCopyBuffer=link);
            var buttons=Panel(canvas.transform,"Presenter controls",new Vector2(1,1),new Vector2(-260,-155),new Vector2(490,185));controls=buttons.gameObject;
            for(int i=0;i<3;i++){int index=i;Button(buttons,new[]{"1 · Interest / scale","2 · Animator sync","3 · Soccer"}[i],new Vector2(0,54-i*49),new Vector2(460,43),()=>presentation.Select(index));}
            Text(buttons,"Slide keys","I: screenshot · PgUp/PgDn: previous/next",new Vector2(0,-76),new Vector2(470,26),14);
            var disconnect=Button(canvas.transform,"Disconnect",new Vector2(-92,-30),new Vector2(160,35),()=>presentation.manager.Stop());Anchor(disconnect.GetComponent<RectTransform>(),new Vector2(1,1));
            var circle=new GameObject("Movement",typeof(RectTransform),typeof(SoccerStickGraphic));circle.transform.SetParent(canvas.transform,false);var rect=circle.GetComponent<RectTransform>();Anchor(rect,Vector2.zero);rect.anchoredPosition=new Vector2(220,210);rect.sizeDelta=new Vector2(220,220);circle.GetComponent<SoccerStickGraphic>().color=new Color(.1f,.2f,.3f,.65f);stick=circle;
            var knob=new GameObject("Handle",typeof(RectTransform),typeof(SoccerStickGraphic));knob.transform.SetParent(rect,false);var handle=knob.GetComponent<RectTransform>();handle.sizeDelta=new Vector2(80,80);knob.GetComponent<SoccerStickGraphic>().color=new Color(.7f,.85f,1,.9f);knob.GetComponent<SoccerStickGraphic>().raycastTarget=false;circle.AddComponent<SoccerStick>().handle=handle;
            var wave=Button(canvas.transform,"Wave / Space",new Vector2(-120,92),new Vector2(200,65),()=>{var owner=presentation.Owner;if(owner)owner.GetComponent<M2Actor>().Wave();});Anchor(wave.GetComponent<RectTransform>(),new Vector2(1,0));
            var overlay=Panel(canvas.transform,"Screenshot overlay",new Vector2(.5f,.5f),new Vector2(120,-15),new Vector2(1100,700));slide=overlay.gameObject;
            var picture=new GameObject("Screenshot",typeof(RectTransform),typeof(UnityEngine.UI.RawImage),typeof(UnityEngine.UI.AspectRatioFitter));picture.transform.SetParent(overlay,false);slideView=picture.GetComponent<UnityEngine.UI.RawImage>();slideView.raycastTarget=false;slideAspect=picture.GetComponent<UnityEngine.UI.AspectRatioFitter>();slideAspect.aspectMode=UnityEngine.UI.AspectRatioFitter.AspectMode.FitInParent;HideScreenshot();
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
