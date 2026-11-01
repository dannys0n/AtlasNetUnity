using AtlasNet;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace AtlasNet.Demos
{
    /// <summary>Runtime view for the local worker harness. Networking remains in LocalWorkerDemo.</summary>
    public sealed class LocalWorkerPanel : MonoBehaviour
    {
        LocalWorkerDemo demo;
        GameObject canvasRoot, content;
        RectTransform panelRect;
        TMP_Text status, details, collapseLabel;
        UnityEngine.UI.Button add;
        UnityEngine.UI.Toggle viewer;
        readonly UnityEngine.UI.Toggle[] filters = new UnityEngine.UI.Toggle[4];
        readonly UnityEngine.UI.Button[] remove = new UnityEngine.UI.Button[4];
        NetworkManager[] servers = System.Array.Empty<NetworkManager>();
        float refresh;
        TMP_FontAsset font;
        public void Initialize(LocalWorkerDemo owner)
        {
            demo=owner;
            font=TMP_Settings.defaultFontAsset;
            canvasRoot=new GameObject("Local workers UI",typeof(RectTransform),typeof(Canvas),typeof(CanvasScaler),typeof(GraphicRaycaster));
            canvasRoot.transform.SetParent(transform,false);
            var canvas=canvasRoot.GetComponent<Canvas>(); canvas.renderMode=RenderMode.ScreenSpaceOverlay; canvas.sortingOrder=100;
            var scaler=canvasRoot.GetComponent<CanvasScaler>(); scaler.uiScaleMode=CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution=new Vector2(1280,720); scaler.matchWidthOrHeight=.5f;
            var panel=new GameObject("Worker controls",typeof(RectTransform),typeof(UnityEngine.UI.Image)).GetComponent<RectTransform>(); panel.SetParent(canvasRoot.transform,false);
            panelRect=panel;
            panel.anchorMin=panel.anchorMax=panel.pivot=new Vector2(0,1); panel.anchoredPosition=new Vector2(16,-16); panel.sizeDelta=new Vector2(370,510); panel.localScale=Vector3.one*.75f;
            panel.GetComponent<UnityEngine.UI.Image>().color=new Color(.035f,.04f,.05f,.97f);
            var handle=Rect(panel,"Window drag handle",0,22);
            handle.anchoredPosition=Vector2.zero; handle.sizeDelta=new Vector2(370,22);
            handle.gameObject.AddComponent<UnityEngine.UI.Image>().color=new Color(.09f,.1f,.12f);
            handle.gameObject.AddComponent<LocalWorkerPanelDrag>().Target=panel;
            var title=Label(handle,"Local workers",0,22); Stretch(title.rectTransform,8);
            title.fontSize=14; title.alignment=TextAlignmentOptions.MidlineLeft;
            var collapse=Button(panel,"Collapse / F8",28,()=>demo.ToggleExpanded());
            collapseLabel=collapse.GetComponentInChildren<TMP_Text>();
            content=new GameObject("Expanded controls",typeof(RectTransform)); content.transform.SetParent(panel,false);
            var cr=(RectTransform)content.transform; cr.anchorMin=Vector2.zero;cr.anchorMax=Vector2.one;cr.offsetMin=cr.offsetMax=Vector2.zero;
            status=Label(cr,"",70,28);
            add=Button(cr,"Add local worker",100,()=>demo.RunAction(demo.AddWorker));
            viewer=Toggle(cr,"Viewer-interest replicas (Server 0)",145,v=>{if(demo.Primary)demo.Primary.ShowViewerInterestForDebug=v;});
            for(int i=0;i<4;i++)
            {
                int slot=i;
                filters[i]=Toggle(cr,"Server",190+i*45,v=>{if(slot<servers.Length)demo.SetServerVisible(servers[slot],v);});
                var r=(RectTransform)filters[i].transform;r.sizeDelta=new Vector2(225,36);
                remove[i]=Button(cr,"Remove",190+i*45,()=>demo.RunAction(()=>{if(slot<servers.Length)demo.RemoveWorker(servers[slot]);}));
                var rr=(RectTransform)remove[i].transform;rr.anchoredPosition=new Vector2(250,-(190+i*45));rr.sizeDelta=new Vector2(105,36);
            }
            details=Label(cr,"",375,118);details.fontSize=15;
            if(!EventSystem.current) { var es=new GameObject("Worker UI EventSystem",typeof(EventSystem),typeof(StandaloneInputModule));es.transform.SetParent(canvasRoot.transform,false); }
        }
        RectTransform Rect(Transform parent,string name,float y,float height)
        {
            var rect=new GameObject(name,typeof(RectTransform)).GetComponent<RectTransform>();rect.SetParent(parent,false);rect.anchorMin=rect.anchorMax=rect.pivot=new Vector2(0,1);rect.anchoredPosition=new Vector2(14,-y);rect.sizeDelta=new Vector2(342,height);return rect;
        }
        static void Stretch(RectTransform rect,float inset=0)
        {
            rect.anchorMin=Vector2.zero; rect.anchorMax=Vector2.one;
            rect.offsetMin=new Vector2(inset,0); rect.offsetMax=new Vector2(-inset,0);
        }
        TMP_Text Label(Transform parent,string text,float y,float height)
        {
            var rect=Rect(parent,"Label",y,height);var label=rect.gameObject.AddComponent<TextMeshProUGUI>();label.font=font;label.fontSize=18;label.color=Color.white;label.text=text;label.raycastTarget=false;label.textWrappingMode=TextWrappingModes.Normal;return label;
        }
        UnityEngine.UI.Button Button(Transform parent,string text,float y,UnityEngine.Events.UnityAction action)
        {
            var rect=Rect(parent,text,y,36);rect.gameObject.AddComponent<UnityEngine.UI.Image>().color=new Color(.16f,.18f,.21f);var button=rect.gameObject.AddComponent<UnityEngine.UI.Button>();button.onClick.AddListener(action);var label=Label(rect,text,0,36);Stretch(label.rectTransform);label.alignment=TextAlignmentOptions.Center;return button;
        }
        UnityEngine.UI.Toggle Toggle(Transform parent,string text,float y,UnityEngine.Events.UnityAction<bool> action)
        {
            var rect=Rect(parent,text,y,36);var bg=rect.gameObject.AddComponent<UnityEngine.UI.Image>();bg.color=new Color(.12f,.14f,.17f);var toggle=rect.gameObject.AddComponent<UnityEngine.UI.Toggle>();toggle.targetGraphic=bg;
            var check=Rect(rect,"Selected",0,36);Stretch(check);var image=check.gameObject.AddComponent<UnityEngine.UI.Image>();image.color=new Color(.65f,.28f,.07f);image.raycastTarget=false;toggle.graphic=image;
            var label=Label(rect,text,0,36);Stretch(label.rectTransform,7);label.alignment=TextAlignmentOptions.MidlineLeft;toggle.onValueChanged.AddListener(action);return toggle;
        }
        void Update()
        {
            if(!demo||!canvasRoot)return;
            canvasRoot.SetActive(demo.Primary&&demo.Primary.IsRunning&&demo.Primary.IsServer);
            content.SetActive(demo.Expanded);
            panelRect.sizeDelta=new Vector2(370,demo.Expanded?510:70);
            collapseLabel.text=demo.Expanded?"Collapse / F8":"Expand / F8";
            if(Time.unscaledTime<refresh)return;refresh=Time.unscaledTime+.25f;
            servers=demo.Servers;status.text=demo.Status;
            add.interactable=demo.Primary&&demo.Primary.IsRunning&&demo.ExtraWorkerCount<3;
            viewer.interactable=demo.Primary&&demo.Primary.IsRunning;
            viewer.SetIsOnWithoutNotify(demo.Primary&&demo.Primary.ShowViewerInterestForDebug);
            for(int i=0;i<4;i++)
            {
                bool exists=i<servers.Length;filters[i].gameObject.SetActive(exists);remove[i].gameObject.SetActive(exists&&servers[i]!=demo.Primary);
                if(!exists)continue;
                filters[i].SetIsOnWithoutNotify(demo.IsServerVisible(servers[i]));
                filters[i].GetComponentInChildren<TMP_Text>().text=servers[i].LocalWorkerId==ulong.MaxValue?"Joining...":$"Server {servers[i].LocalWorkerId}";
                remove[i].interactable=!demo.IsDraining(servers[i])&&servers[i].LocalWorkerId!=ulong.MaxValue;
                remove[i].GetComponentInChildren<TMP_Text>().text=demo.IsDraining(servers[i])?"Draining...":"Remove";
            }
            details.text=string.Join("\n",demo.Rows)+"\nIsolated physics worlds / loopback UDP.\nOne process, one main thread; not parallel CPU scaling.";
        }
    }
    public sealed class LocalWorkerPanelDrag : MonoBehaviour, IBeginDragHandler, IDragHandler
    {
        public RectTransform Target;
        Vector2 offset;
        public void OnBeginDrag(PointerEventData data)
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle((RectTransform)Target.parent,data.position,data.pressEventCamera,out var point);
            offset=Target.anchoredPosition-point;
        }
        public void OnDrag(PointerEventData data)
        {
            var parent=(RectTransform)Target.parent;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(parent,data.position,data.pressEventCamera,out var point);
            var position=point+offset;
            position.x=Mathf.Clamp(position.x,0,Mathf.Max(0,parent.rect.width-Target.rect.width*Target.localScale.x));
            position.y=Mathf.Clamp(position.y,-Mathf.Max(0,parent.rect.height-22*Target.localScale.y),0);
            Target.anchoredPosition=position;
        }
    }
}
