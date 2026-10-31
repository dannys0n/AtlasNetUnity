using AtlasNet;
using UnityEngine;

namespace AtlasNet.Samples.Soccer
{
    // Local presentation only. Each player sees a different attacking goal; nothing is networked here.
    public sealed class SoccerGoalPointer : MonoBehaviour
    {
        public NetworkManager manager;
        public Mesh arrowMesh;
        public Material arrowMaterial;
        SoccerPlayer player;
        SoccerBall ball;
        GameObject pointer;
        SoccerBallScreenArrow screenArrow;
        Canvas hud;
        Camera view;
        readonly Plane[] frustum=new Plane[6];
        MaterialPropertyBlock tint;
        int shownTeam=-1;
        void LateUpdate()
        {
            if (!manager.IsRunning || !manager.IsClient)
            {
                player=null;ball=null;HidePointers();return;
            }
            if (!player || !player.IsSpawned || !ball || !ball.IsSpawned)
            {
                foreach(var obj in manager.SpawnedObjects)
                {
                    if (obj.IsOwner && obj.TryGetComponent(out SoccerPlayer own))player=own;
                    if (obj.TryGetComponent(out SoccerBall shared))ball=shared;
                }
            }
            if (!player || !player.IsSpawned || !ball || !ball.IsSpawned)
            { HidePointers();return; }
            if (!pointer)
            {
                pointer=new GameObject("Attacking goal arrow",typeof(MeshFilter),typeof(MeshRenderer));
                pointer.transform.SetParent(transform,false);
                pointer.GetComponent<MeshFilter>().sharedMesh=arrowMesh;
                pointer.GetComponent<MeshRenderer>().sharedMaterial=arrowMaterial;
            }
            pointer.SetActive(true);
            if(shownTeam!=player.Team)
            {
                tint??=new MaterialPropertyBlock();var color=player.Team==0?SoccerSession.Blue:SoccerSession.Orange;
                tint.SetColor("_Color",color);tint.SetColor("_BaseColor",color);pointer.GetComponent<MeshRenderer>().SetPropertyBlock(tint);shownTeam=player.Team;
            }
            Vector3 direction=player.AttackingGoal-ball.transform.position;direction.y=0;
            if(direction.sqrMagnitude>.001f)
                pointer.transform.SetPositionAndRotation(ball.transform.position,Quaternion.LookRotation(direction));
            UpdateOffscreenArrow();
        }
        void UpdateOffscreenArrow()
        {
            if(!view)view=Camera.main;
            if(!view){if(screenArrow)screenArrow.gameObject.SetActive(false);return;}
            GeometryUtility.CalculateFrustumPlanes(view,frustum);
            // Hide even when only part of the ball is visible.
            if(GeometryUtility.TestPlanesAABB(frustum,new Bounds(ball.transform.position,Vector3.one*(SoccerBall.Radius*2))))
            {if(screenArrow)screenArrow.gameObject.SetActive(false);return;}
            if(!hud)hud=GetComponentInChildren<Canvas>();
            if(!hud)return;
            if(!screenArrow)
            {
                var obj=new GameObject("Off-screen ball arrow",typeof(RectTransform),typeof(SoccerBallScreenArrow));
                obj.transform.SetParent(hud.transform,false);screenArrow=obj.GetComponent<SoccerBallScreenArrow>();
                screenArrow.raycastTarget=false;screenArrow.rectTransform.sizeDelta=new Vector2(42,54);
                screenArrow.rectTransform.anchorMin=screenArrow.rectTransform.anchorMax=new Vector2(.5f,.5f);
            }
            Rect visible=view.pixelRect;Rect safe=Screen.safeArea;
            float margin=58*hud.scaleFactor;
            var edge=Rect.MinMaxRect(Mathf.Max(visible.xMin,safe.xMin)+margin,Mathf.Max(visible.yMin,safe.yMin)+margin,
                Mathf.Min(visible.xMax,safe.xMax)-margin,Mathf.Min(visible.yMax,safe.yMax)-100*hud.scaleFactor);
            if(edge.width<=0 || edge.height<=0){screenArrow.gameObject.SetActive(false);return;}
            Vector2 from=view.WorldToScreenPoint(player.transform.position+Vector3.up*1.5f);
            Vector3 projected=view.WorldToScreenPoint(ball.transform.position);
            Vector2 toward=(Vector2)projected-from;
            if(projected.z<=0)toward=-toward;
            if(toward.sqrMagnitude<.001f)toward=Vector2.down;
            toward.Normalize();
            from.x=Mathf.Clamp(from.x,edge.xMin,edge.xMax);from.y=Mathf.Clamp(from.y,edge.yMin,edge.yMax);
            float horizontal=Mathf.Abs(toward.x)>.0001f?((toward.x>0?edge.xMax:edge.xMin)-from.x)/toward.x:float.PositiveInfinity;
            float vertical=Mathf.Abs(toward.y)>.0001f?((toward.y>0?edge.yMax:edge.yMin)-from.y)/toward.y:float.PositiveInfinity;
            Vector2 point=from+toward*Mathf.Min(horizontal,vertical);
            var uiCamera=hud.renderMode==RenderMode.ScreenSpaceOverlay?null:hud.worldCamera;
            RectTransformUtility.ScreenPointToLocalPointInRectangle((RectTransform)hud.transform,point,uiCamera,out var local);
            screenArrow.rectTransform.anchoredPosition=local;
            screenArrow.rectTransform.localRotation=Quaternion.Euler(0,0,Mathf.Atan2(toward.y,toward.x)*Mathf.Rad2Deg-90);
            screenArrow.color=player.Team==0?SoccerSession.Blue:SoccerSession.Orange;
            screenArrow.gameObject.SetActive(true);
        }
        void HidePointers(){if(pointer)pointer.SetActive(false);if(screenArrow)screenArrow.gameObject.SetActive(false);}
        void OnDisable()=>HidePointers();
        void OnDestroy(){if(pointer)Destroy(pointer);if(screenArrow)Destroy(screenArrow.gameObject);}
    }
}